using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotInputProbeWindow : EditorWindow
    {
        public int First;
        public int Second;
        internal Color Background = Color.magenta;
        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), Background);
            var first = new Rect(15, 30, 120, 25);
            var second = new Rect(15, 65, 120, 25);
            if (GUI.Button(first, "Stop")) First++;
            if (GUI.Button(second, "Stop")) Second++;
            if (Event.current.type == EventType.Repaint)
            {
                using (var frame = UPilotWindowInputRegistry.BeginFrame(this))
                {
                    frame.Add("Stop", "Button", first, new Rect(Vector2.zero, position.size), true);
                    frame.Add("Stop", "Button", second, new Rect(Vector2.zero, position.size), true);
                    frame.Add("Disabled", "Button", new Rect(15, 100, 120, 25), new Rect(Vector2.zero, position.size), false);
                    frame.Add("Clipped", "Button", new Rect(15, 300, 120, 25), new Rect(Vector2.zero, position.size), true);
                }
            }
        }
    }

    public sealed class UPilotWindowGeometryMarkerProbe : EditorWindow
    {
        internal const int MarkerThickness = 8;

        private void OnGUI()
        {
            var width = Mathf.Max(1f, position.width);
            var height = Mathf.Max(1f, position.height);
            EditorGUI.DrawRect(new Rect(0, 0, width, height), Color.magenta);
            EditorGUI.DrawRect(new Rect(0, 0, MarkerThickness, height), Color.red);
            EditorGUI.DrawRect(new Rect(width - MarkerThickness, 0, MarkerThickness, height), Color.green);
            EditorGUI.DrawRect(new Rect(0, 0, width, MarkerThickness), Color.blue);
            EditorGUI.DrawRect(new Rect(0, height - MarkerThickness, width, MarkerThickness), Color.yellow);
        }
    }

    public sealed class UPilotFailingOnEnableWindowProbe : EditorWindow
    {
        internal static bool ThrowOnEnable;

        private void OnEnable()
        {
            if (ThrowOnEnable)
                throw new InvalidOperationException("UPILOT_P2_WINDOW_ON_ENABLE_PROBE");
        }
    }

    public sealed class UPilotWindowInputContractTests
    {
        [Test]
        public void MouseReceiptAndPendingModalRetainConcreteWireFields()
        {
            var receipt = new GenericOkPayload { ok = true, input = new WindowInputEvidence
                { dispatched = true, businessEffectVerified = false, dispatchRoute = "uitoolkit" } };
            var json = JsonUtility.ToJson(new ResultMessage<GenericOkPayload> { payload = receipt });
            var restored = JsonUtility.FromJson<ResultMessage<GenericOkPayload>>(json);
            Assert.That(restored.payload.input.dispatched, Is.True);
            Assert.That(restored.payload.input.businessEffectVerified, Is.False);
            Assert.That(restored.payload.input.dispatchRoute, Is.EqualTo("uitoolkit"));
            var modalJson = JsonUtility.ToJson(new ResultMessage<ModalObservationPayload>
                { payload = new ModalObservationPayload { commandId = "mouse-probe", waitingForModalUi = true } });
            var modal = JsonUtility.FromJson<ResultMessage<ModalObservationPayload>>(modalJson);
            Assert.That(modal.payload.commandId, Is.EqualTo("mouse-probe"));
            Assert.That(modal.payload.waitingForModalUi, Is.True);
        }

        [Test]
        public void AuthoritativeSnapshotCarriesTheLedgerTransition()
        {
            var method = typeof(UPilotBridge).GetMethod("BuildExecutionStatePayload",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var snapshot = (EditorExecutionStatePayload)method.Invoke(UPilotBridge.Instance,
                new object[] { "test_transition_contract" });
            Assert.That(snapshot.playModeTransition, Is.SameAs(UPilotPlayModeTransitions.Latest),
                "The ordered authoritative snapshot must retain the producer's ledger record.");
        }

        private static UPilotBridge CreateIsolatedModeBridge()
        {
            // Constructors restore shared state and subscribe live callbacks; do neither here.
            var bridge = (UPilotBridge)FormatterServices.GetUninitializedObject(typeof(UPilotBridge));
            var compile = (UPilotCompileService)FormatterServices.GetUninitializedObject(typeof(UPilotCompileService));
            typeof(UPilotCompileService).GetField("_lastErrors", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(compile, new List<CompileErrorItemPayload>());
            SetIsolatedModeField(bridge, "_compileService", compile);
            SetIsolatedModeField(bridge, "_mainThreadQueue", new ConcurrentQueue<Action>());
            SetIsolatedModeField(bridge, "_producerEpoch", "isolated-mode-test");
            SetIsolatedModeField(bridge, "_domainGeneration", 7L);
            SetIsolatedModeField(bridge, "_stateSequence", 100L);
            SetIsolatedModeField(bridge, "_lastMainThreadPumpAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return bridge;
        }

        private static void SetIsolatedModeField(UPilotBridge bridge, string name, object value) =>
            typeof(UPilotBridge).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(bridge, value);

        [TestCase(true)]
        [TestCase(false)]
        public void OrderedPauseResumeSnapshotsRetainIdentityAndMode(bool initiallyPaused)
        {
            var bridge = CreateIsolatedModeBridge();
            var build = typeof(UPilotBridge).GetMethod("BuildExecutionStatePayload",
                BindingFlags.Instance | BindingFlags.NonPublic);
            SetIsolatedModeField(bridge, "_cachedIsPlaying", true);
            SetIsolatedModeField(bridge, "_cachedIsPaused", initiallyPaused);
            SetIsolatedModeField(bridge, "_cachedPlayModeState", initiallyPaused ? "pause" : "play");
            var before = (EditorExecutionStatePayload)build.Invoke(bridge, new object[] { "before_pause_change" });
            SetIsolatedModeField(bridge, "_cachedIsPaused", !initiallyPaused);
            SetIsolatedModeField(bridge, "_cachedPlayModeState", initiallyPaused ? "play" : "pause");
            var after = (EditorExecutionStatePayload)build.Invoke(bridge, new object[] { "pause_state_changed" });
            var restored = JsonUtility.FromJson<EditorExecutionStatePayload>(JsonUtility.ToJson(after));

            Assert.That(before.snapshotId, Is.EqualTo("isolated-mode-test:7:101"));
            Assert.That(before.playModeState, Is.EqualTo(initiallyPaused ? "pause" : "play"));
            Assert.That(before.isPaused, Is.EqualTo(initiallyPaused));
            Assert.That(restored.snapshotId, Is.EqualTo("isolated-mode-test:7:102"));
            Assert.That(restored.sequence, Is.EqualTo(before.sequence + 1));
            Assert.That(restored.producerEpoch, Is.EqualTo(before.producerEpoch));
            Assert.That(restored.domainGeneration, Is.EqualTo(before.domainGeneration));
            Assert.That(restored.playModeState, Is.EqualTo(initiallyPaused ? "play" : "pause"));
            Assert.That(restored.isPaused, Is.EqualTo(!initiallyPaused));
            Assert.That(restored.isPlaying, Is.True);
            Assert.That(restored.transition, Is.EqualTo("pause_state_changed"));
        }

        [TestCase(PauseState.Paused)]
        [TestCase(PauseState.Unpaused)]
        public void InactivePauseCallbackObservesEditorInsteadOfRequestedState(PauseState notification)
        {
            var bridge = CreateIsolatedModeBridge();
            SetIsolatedModeField(bridge, "_cachedIsPlaying", !EditorApplication.isPlaying);
            SetIsolatedModeField(bridge, "_cachedIsPaused", !EditorApplication.isPaused);
            SetIsolatedModeField(bridge, "_cachedPlayModeState", "unknown");
            typeof(UPilotBridge).GetMethod("OnPauseStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bridge, new object[] { notification });
            var context = bridge.GetEditorExecutionContext("isolated_pause_callback");

            Assert.That(context.isPlaying, Is.EqualTo(EditorApplication.isPlaying));
            Assert.That(context.isPaused, Is.EqualTo(EditorApplication.isPaused));
            Assert.That(context.playModeState, Is.EqualTo(EditorApplication.isPaused ? "pause" :
                (EditorApplication.isPlaying ? "play" : "edit")));
            Assert.That(context.sequence, Is.EqualTo(100), "An inactive Bridge must not publish or persist a snapshot.");
            Assert.That(context.connected, Is.False);
            Assert.That(context.updatedAt, Is.GreaterThan(0));
        }

        [Test]
        public void ExecutionStatePersistenceSerializesConcurrentWritesAndCleansTemporaryFiles()
        {
            var directory = Path.Combine(Path.GetTempPath(), "UPilotExecutionStateTests");
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            var method = typeof(UPilotBridge).GetMethod("PersistExecutionState",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(EditorExecutionStatePayload), typeof(string) },
                null);
            Assert.That(method, Is.Not.Null);

            try
            {
                var succeeded = new bool[32];
                Parallel.For(0, succeeded.Length, index =>
                {
                    succeeded[index] = (bool)method.Invoke(null, new object[]
                    {
                        new EditorExecutionStatePayload
                        {
                            processId = 1,
                            sequence = index + 1,
                            transition = "concurrent_test",
                        },
                        path,
                    });
                });

                foreach (var result in succeeded)
                    Assert.That(result, Is.True);

                var persisted = JsonUtility.FromJson<EditorExecutionStatePayload>(File.ReadAllText(path));
                Assert.That(persisted, Is.Not.Null);
                Assert.That(persisted.sequence, Is.InRange(1, succeeded.Length));
                Assert.That(Directory.GetFiles(directory, Path.GetFileName(path) + ".*.tmp"), Is.Empty);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    foreach (var temporary in Directory.GetFiles(directory, Path.GetFileName(path) + ".*.tmp"))
                        File.Delete(temporary);
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }

        public sealed class ScrollProbe : EditorWindow
        {
            public Vector2 Scroll;
            public int Clicks;
            private void OnGUI()
            {
                var clip = new Rect(10, 15, 240, 150);
                var button = new Rect(20, 350, 130, 25);
                Scroll = GUI.BeginScrollView(clip, Scroll, new Rect(0, 0, 210, 600));
                if (GUI.Button(button, "Scrolled action")) Clicks++;
                GUI.EndScrollView();
                if (Event.current.type == EventType.Repaint)
                {
                    using (var frame = UPilotWindowInputRegistry.BeginFrame(this))
                        frame.Add("Scrolled action", "Button",
                            new Rect(clip.position + button.position - Scroll, button.size), clip, GUI.enabled);
                }
            }
        }

        [UnityTest]
        public IEnumerator RegisteredScrolledControlsDispatchExactlyAndRejectClippedTargets()
        {
            var window = ScriptableObject.CreateInstance<ScrollProbe>();
            try
            {
                window.titleContent = new GUIContent("UPilot scroll probe");
                window.ShowUtility();
                window.position = new Rect(150, 150, 300, 220);
                var input = new UPilotPlayInputService();
                for (var i = 0; i < 20; i++)
                {
                    window.Scroll = new Vector2(0, 300 + i);
                    window.Repaint();
                    yield return null;
                    var result = input.HandleMouseEvent(new MouseEventPayload
                    {
                        action = "click", button = "left",
                        windowInstanceId = UPilotEntityIds.ToWireId(window).ToString(),
                        elementName = "Scrolled action", elementIndex = -1,
                    });
                    Assert.That(result.ok, Is.True, result.state);
                    Assert.That(window.Clicks, Is.EqualTo(i + 1));
                }
                window.Scroll = Vector2.zero;
                window.Repaint();
                yield return null;
                var clipped = input.HandleMouseEvent(new MouseEventPayload
                {
                    action = "click", button = "left",
                    windowInstanceId = UPilotEntityIds.ToWireId(window).ToString(),
                    elementName = "Scrolled action", elementIndex = -1,
                });
                Assert.That(clipped.ok, Is.False);
                Assert.That(clipped.state, Does.Contain("ELEMENT_NOT_VISIBLE"));
                Assert.That(window.Clicks, Is.EqualTo(20));
            }
            finally { if (window != null) window.Close(); }
        }

        [UnityTest]
        public IEnumerator ExactInstanceAndDuplicateButtonSelectionNeverUseFocusFallback()
        {
            var first = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            var other = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            try
            {
                first.titleContent = other.titleContent = new GUIContent("UPilot input probe");
                first.ShowUtility(); other.ShowUtility();
                first.position = new Rect(100, 100, 320, 220);
                other.position = new Rect(500, 100, 320, 220);
                var input = new UPilotPlayInputService();
                for (int i = 0; i < 20; i++)
                {
                    first.Repaint();
                    yield return null;
                    var result = input.HandleMouseEvent(new MouseEventPayload
                    {
                        action = "click", button = "left", windowInstanceId = UPilotEntityIds.ToWireId(first).ToString(),
                        elementName = "Stop", elementIndex = 1,
                    });
                    Assert.That(result.ok, Is.True, result.state);
                    Assert.That(result.input.dispatched, Is.True);
                    Assert.That(result.input.businessEffectVerified, Is.False);
                }
                Assert.That(first.First, Is.Zero);
                Assert.That(first.Second, Is.EqualTo(20));
                Assert.That(other.Second, Is.Zero);
                foreach (var name in new[] { "Stop", "Disabled", "Clipped" })
                {
                    var rejected = input.HandleMouseEvent(new MouseEventPayload
                    {
                        action = "click", button = "left", windowInstanceId = UPilotEntityIds.ToWireId(first).ToString(),
                        elementName = name, elementIndex = -1,
                    });
                    Assert.That(rejected.ok, Is.False, name);
                }
                var failed = input.HandleMouseEvent(new MouseEventPayload
                { action = "click", button = "left", windowInstanceId = UPilotEntityIds.ToWireId(first).ToString(), elementName = "missing" });
                Assert.That(failed.ok, Is.False);
                Assert.That(first.Second, Is.EqualTo(20));
                Assert.That(UPilotWindowInputRegistry.Resolve("0", "UPilot input probe"), Is.Null);
                var handle = UPilotWindowInputRegistry.Handle(first);
                Assert.That(UPilotWindowInputRegistry.ResolveHandle(handle), Is.SameAs(first));
                first.Close();
                Assert.That(UPilotWindowInputRegistry.ResolveHandle(handle), Is.Null);
                Assert.That(UPilotWindowInputRegistry.ResolveHandle("window:old:42"), Is.Null);
            }
            finally
            {
                if (first != null) first.Close();
                if (other != null) other.Close();
            }
        }

        public sealed class MixedInputProbe : EditorWindow
        {
            public int ToolkitClicks;
            public int ImguiClicks;
            public void CreateGUI()
            {
                var button = new UnityEngine.UIElements.Button(() => ToolkitClicks++) { name = "toolkit", text = "Toolkit" };
                button.style.height = 40;
                rootVisualElement.Add(button);
                var imgui = new UnityEngine.UIElements.IMGUIContainer(() =>
                {
                    if (GUILayout.Button("IMGUI", GUILayout.Height(40))) ImguiClicks++;
                }) { name = "imgui" };
                imgui.style.height = 60;
                rootVisualElement.Add(imgui);
            }
        }

        [UnityTest]
        public IEnumerator CoordinateMouseUsesActualHitAndOnlyOneRoute()
        {
            var window = ScriptableObject.CreateInstance<MixedInputProbe>();
            try
            {
                window.ShowUtility();
                window.position = new Rect(150, 150, 320, 220);
                for (var i = 0; i < 3; i++) { window.Repaint(); yield return null; }
                var input = new UPilotPlayInputService();
                foreach (var y in new[] { 20f, 60f })
                {
                    var result = input.HandleMouseEvent(new MouseEventPayload
                    {
                        action = "click", button = "left", x = 100, y = y, elementIndex = -1,
                        windowInstanceId = UPilotEntityIds.ToWireId(window).ToString(),
                    });
                    Assert.That(result.ok, Is.True, result.state);
                    Assert.That(result.input.dispatched, Is.True);
                    Assert.That(result.input.hitVerified, Is.True);
                    Assert.That(result.input.enabled, Is.True);
                    Assert.That(result.input.dispatchRoute, Is.EqualTo(y == 20 ? "uitoolkit" : "imgui"));
                    Assert.That(result.input.businessEffectVerified, Is.False);
                    yield return null;
                }
                Assert.That(window.ToolkitClicks, Is.EqualTo(1));
                Assert.That(window.ImguiClicks, Is.EqualTo(1));
                window.rootVisualElement[0].SetEnabled(false);
                var disabled = input.HandleMouseEvent(new MouseEventPayload {
                    action = "click", button = "left", x = 100, y = 20, elementIndex = -1,
                    windowInstanceId = UPilotEntityIds.ToWireId(window).ToString(),
                });
                Assert.That(disabled.ok, Is.False);
                Assert.That(disabled.input.enabled, Is.False);
                Assert.That(disabled.input.dispatched, Is.False);
                Assert.That(window.ToolkitClicks, Is.EqualTo(1));
            }
            finally { if (window != null) window.Close(); }
        }

        [UnityTest]
        public IEnumerator NativeCaptureMatchesWindowPixelsAcrossMoves()
        {
            var window = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            var overlap = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            var previousFocus = EditorWindow.focusedWindow;
            var evidenceDirectory = Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                "Log", "Stage4", "WindowOcclusion", Guid.NewGuid().ToString("N"));
            try
            {
                window.titleContent = overlap.titleContent = new GUIContent("UPilot same-name capture probe");
                overlap.Background = Color.green;
                window.ShowUtility(); overlap.ShowUtility();
                window.position = new Rect(100, 100, 320, 220);
                overlap.position = new Rect(160, 160, 320, 220);
                for (int i = 0; i < 20; i++)
                {
                    window.position = new Rect(100 + i * 2, 100, 320 + i * 2, 220 + i);
                    window.Repaint(); overlap.Repaint(); overlap.Focus();
                    yield return null;
                    var capture = UPilotWindowDiagnostics.CaptureEditorWindowPixels(window, false);
                    Assert.That(capture.pixelSourceVerified, Is.True);
                    Assert.That(capture.contentRectVerified, Is.True);
                    Assert.That(capture.cropComplete, Is.True);
                    Assert.That(capture.foreground, Is.False, "The target must actually be behind the owned occluder.");
                    Assert.That(capture.width, Is.EqualTo(Mathf.RoundToInt(window.position.width * capture.pixelsPerPoint)));
                    Assert.That(capture.height, Is.EqualTo(Mathf.RoundToInt(window.position.height * capture.pixelsPerPoint)));
                    Assert.That(capture.occlusionSensitive, Is.False);
                    Assert.That(capture.instanceId, Is.EqualTo(UPilotEntityIds.ToWireId(window)));
                    Assert.That(capture.windowHandle, Is.Not.Zero);
                    if (i == 0 || i == 19)
                    {
                        var occluder = UPilotWindowDiagnostics.CaptureEditorWindowPixels(overlap, false);
                        Assert.That(occluder.foreground, Is.True, "The owned green window must be the native foreground window.");
                        Assert.That(occluder.windowHandle, Is.Not.EqualTo(capture.windowHandle));
                        Directory.CreateDirectory(evidenceDirectory);
                        File.WriteAllBytes(Path.Combine(evidenceDirectory, $"target-{i}.png"), Convert.FromBase64String(capture.imageData));
                        File.WriteAllBytes(Path.Combine(evidenceDirectory, $"occluder-{i}.png"), Convert.FromBase64String(occluder.imageData));
                        occluder.imageData = null;
                        File.WriteAllText(Path.Combine(evidenceDirectory, $"occluder-{i}.json"), JsonUtility.ToJson(occluder, true));
                    }
                    var texture = new Texture2D(2, 2);
                    try
                    {
                        Assert.That(texture.LoadImage(Convert.FromBase64String(capture.imageData)), Is.True);
                        Color pixel = texture.GetPixel(texture.width / 2, texture.height / 2);
                        Assert.That(pixel.r, Is.GreaterThan(.9f));
                        Assert.That(pixel.b, Is.GreaterThan(.9f));
                        Assert.That(pixel.g, Is.LessThan(.1f));
                    }
                    finally { UnityEngine.Object.DestroyImmediate(texture); }
                    if (i == 0 || i == 19)
                    {
                        capture.imageData = null;
                        File.WriteAllText(Path.Combine(evidenceDirectory, $"target-{i}.json"), JsonUtility.ToJson(capture, true));
                    }
                }
            }
            finally
            {
                if (overlap != null) overlap.Close();
                if (window != null) window.Close();
                if (previousFocus != null) previousFocus.Focus();
            }
        }

        [UnityTest]
        public IEnumerator ForbiddenFallbackNeverProducesScreenPixels()
        {
            var window = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            var previous = UPilotWindowDiagnostics.RejectNativeCaptureForTesting;
            try
            {
                window.position = new Rect(100, 100, 320, 220);
                window.ShowUtility(); window.Repaint();
                yield return null;
                UPilotWindowDiagnostics.RejectNativeCaptureForTesting = _ => true;
                for (int i = 0; i < 20; i++)
                {
                    var error = Assert.Throws<EditorWindowCaptureException>(() =>
                        UPilotWindowDiagnostics.CaptureEditorWindowPixels(window, false));
                    Assert.That(error.Diagnostics.imageData, Is.Null.Or.Empty);
                    Assert.That(error.Diagnostics.pixelSourceVerified, Is.False);
                    Assert.That(error.Diagnostics.instanceId, Is.EqualTo(UPilotEntityIds.ToWireId(window)));
                }
            }
            finally
            {
                UPilotWindowDiagnostics.RejectNativeCaptureForTesting = previous;
                if (window != null) window.Close();
            }
        }

        [UnityTest]
        public IEnumerator NativeCaptureRejectsGeometryChangedAfterPixelRead()
        {
            var window = ScriptableObject.CreateInstance<UPilotWindowGeometryMarkerProbe>();
            var previous = UPilotWindowDiagnostics.AfterNativeCaptureForTesting;
            try
            {
                window.titleContent = new GUIContent("UPilot geometry-change probe");
                window.position = new Rect(120, 120, 320, 220);
                window.ShowUtility();
                window.Repaint();
                yield return null;

                var original = window.position;
                UPilotWindowDiagnostics.AfterNativeCaptureForTesting = target =>
                    target.position = new Rect(original.x + 24, original.y, original.width, original.height);
                var error = Assert.Throws<EditorWindowCaptureException>(() =>
                    UPilotWindowDiagnostics.CaptureEditorWindowPixels(window, false));
                Assert.That(error.Diagnostics.originalError, Is.EqualTo("WINDOW_CHANGED_DURING_CAPTURE"));
                Assert.That(error.Diagnostics.pixelSourceVerified, Is.False);
                Assert.That(error.Diagnostics.instanceId, Is.EqualTo(UPilotEntityIds.ToWireId(window)));
            }
            finally
            {
                UPilotWindowDiagnostics.AfterNativeCaptureForTesting = previous;
                if (window != null) window.Close();
            }
        }

        [UnityTest]
        public IEnumerator NativeCaptureIncludesAllFourGeometryMarkerEdges()
        {
            var window = ScriptableObject.CreateInstance<UPilotWindowGeometryMarkerProbe>();
            try
            {
                window.titleContent = new GUIContent("UPilot four-edge marker probe");
                window.position = new Rect(160, 160, 320, 220);
                window.ShowUtility();
                window.Repaint();
                yield return null;

                var capture = UPilotWindowDiagnostics.CaptureEditorWindowPixels(window, false);
                Assert.That(capture.contentRectVerified, Is.True);
                Assert.That(capture.cropComplete, Is.True);
                var texture = new Texture2D(2, 2);
                try
                {
                    Assert.That(texture.LoadImage(Convert.FromBase64String(capture.imageData)), Is.True);
                    var inset = Mathf.Clamp(
                        Mathf.RoundToInt(UPilotWindowGeometryMarkerProbe.MarkerThickness * capture.pixelsPerPoint / 2f),
                        1,
                        Math.Min(texture.width, texture.height) / 4);
                    var left = texture.GetPixel(inset, texture.height / 2);
                    var right = texture.GetPixel(texture.width - 1 - inset, texture.height / 2);
                    var verticalA = texture.GetPixel(texture.width / 2, inset);
                    var verticalB = texture.GetPixel(texture.width / 2, texture.height - 1 - inset);

                    Assert.That(left.r, Is.GreaterThan(.8f));
                    Assert.That(left.g, Is.LessThan(.2f));
                    Assert.That(right.g, Is.GreaterThan(.8f));
                    Assert.That(right.r, Is.LessThan(.2f));
                    Assert.That(
                        (verticalA.b > .8f && verticalB.r > .8f && verticalB.g > .8f)
                        || (verticalB.b > .8f && verticalA.r > .8f && verticalA.g > .8f),
                        Is.True,
                        "The top/bottom markers must remain distinct blue and yellow edges regardless of PNG row orientation.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            finally
            {
                if (window != null) window.Close();
            }
        }

        [Test]
        public void SnapshotFailureCodePreservesGeometryChangeAndDoesNotPromoteArbitraryNativeErrors()
        {
            var changed = new EditorWindowCaptureException("changed", new EditorWindowPixelCapture
            {
                originalError = "WINDOW_CHANGED_DURING_CAPTURE",
            });
            var nativeFailure = new EditorWindowCaptureException("failed", new EditorWindowPixelCapture
            {
                originalError = "PRINT_WINDOW_FAILED:5",
            });

            Assert.That(UPilotSnapshotService.CaptureFailureCode(changed), Is.EqualTo("WINDOW_CHANGED_DURING_CAPTURE"));
            Assert.That(UPilotSnapshotService.CaptureFailureCode(nativeFailure), Is.EqualTo("SNAPSHOT_TARGET_CAPTURE_FAILED"));
            Assert.That(UPilotSnapshotService.CaptureFailureCode(new InvalidOperationException()),
                Is.EqualTo("SNAPSHOT_TARGET_CAPTURE_FAILED"));
        }

        [UnityTest]
        public IEnumerator WindowHistoryDoesNotInventAuthoritativeAttributionForOnEnableFailure()
        {
            var before = UPilotWindowHistory.Query(string.Empty, 0, 512);
            UPilotFailingOnEnableWindowProbe window = null;
            try
            {
                UPilotFailingOnEnableWindowProbe.ThrowOnEnable = true;
                LogAssert.Expect(LogType.Exception, new Regex("UPILOT_P2_WINDOW_ON_ENABLE_PROBE"));
                window = ScriptableObject.CreateInstance<UPilotFailingOnEnableWindowProbe>();
                var instanceId = window == null ? string.Empty : UPilotEntityIds.ToWireId(window).ToString();
                yield return null;

                var history = UPilotWindowHistory.Query(instanceId, before.latestSequence, 512);
                Assert.That(history.events.All(item => !item.failureReasonAuthoritative), Is.True);
                Assert.That(history.events.All(item =>
                    string.IsNullOrEmpty(item.failureReason)
                    || !item.failureReason.Contains("UPILOT_P2_WINDOW_ON_ENABLE_PROBE")), Is.True);
            }
            finally
            {
                UPilotFailingOnEnableWindowProbe.ThrowOnEnable = false;
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void TransitionOriginsAreEvidenceBased()
        {
            var unknown = UPilotPlayModeTransitions.Build("play", "edit", "session", 100, null);
            Assert.That(unknown.origin, Is.EqualTo("unknown"));
            foreach (var origin in new[] { "upilot", "testFramework", "project" })
            {
                var record = UPilotPlayModeTransitions.Build("play", "edit", "session", 101,
                    new PlayModeTransitionRecord { origin = origin, requestId = "request", commandId = "command", operationId = "operation" });
                Assert.That(record.origin, Is.EqualTo(origin));
                Assert.That(record.operationId, Is.EqualTo("operation"));
                Assert.That(record.transitionId, Is.Not.EqualTo(unknown.transitionId));
            }
        }

        [Test]
        public void RunnerExitIntentRequiresKnownPlayModeRunAndNoExitAlreadyInProgress()
        {
            var result = new TestRunResultPayload { runGuid = "current-run", testMode = "PlayMode" };
            Assert.That(UPilotTestService.ShouldRecordFrameworkExit(result, true), Is.True);
            Assert.That(UPilotTestService.ShouldRecordFrameworkExit(result, false), Is.False);
            result.testMode = "EditMode";
            Assert.That(UPilotTestService.ShouldRecordFrameworkExit(result, true), Is.False);
            result.testMode = "PlayMode";
            result.runGuid = "";
            Assert.That(UPilotTestService.ShouldRecordFrameworkExit(result, true), Is.False);
        }
    }
}
