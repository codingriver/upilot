using System;
using System.Collections;
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
        private void OnGUI()
        {
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), Color.magenta);
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

    public sealed class UPilotWindowInputContractTests
    {
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

        [UnityTest]
        public IEnumerator NativeCaptureMatchesWindowPixelsAcrossMoves()
        {
            var window = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            var overlap = ScriptableObject.CreateInstance<UPilotInputProbeWindow>();
            try
            {
                window.titleContent = overlap.titleContent = new GUIContent("UPilot same-name capture probe");
                window.ShowUtility(); overlap.ShowUtility();
                window.position = new Rect(100, 100, 320, 220);
                overlap.position = new Rect(250, 200, 320, 220);
                for (int i = 0; i < 20; i++)
                {
                    window.position = new Rect(100 + i * 2, 100, 320, 220);
                    window.Repaint(); overlap.Repaint();
                    yield return null;
                    var capture = UPilotWindowDiagnostics.CaptureEditorWindowPixels(window, false);
                    Assert.That(capture.pixelSourceVerified, Is.True);
                    Assert.That(capture.occlusionSensitive, Is.False);
                    Assert.That(capture.instanceId, Is.EqualTo(UPilotEntityIds.ToWireId(window)));
                    Assert.That(capture.windowHandle, Is.Not.Zero);
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
                }
            }
            finally
            {
                if (overlap != null) overlap.Close();
                if (window != null) window.Close();
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
