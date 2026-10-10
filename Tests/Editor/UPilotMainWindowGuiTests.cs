using System;
using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    public sealed class UPilotMainWindowGuiTests
    {
        [Test]
        public void ExitGuiControlFlowIncludesReflectionWrappers()
        {
            // The constructor changes Unity GUI global state; classification needs only the type.
            var exit = (ExitGUIException)FormatterServices.GetUninitializedObject(typeof(ExitGUIException));
            Assert.That(UPilotMainWindow.IsExitGUIException(exit), Is.True);
            Assert.That(UPilotMainWindow.IsExitGUIException(new TargetInvocationException(exit)), Is.True);
            Assert.That(UPilotMainWindow.IsExitGUIException(
                new TargetInvocationException(new TargetInvocationException(exit))), Is.True);
            Assert.That(UPilotMainWindow.IsExitGUIException(new InvalidOperationException("draw failed")), Is.False);
            Assert.That(UPilotMainWindow.IsExitGUIException(
                new TargetInvocationException(new InvalidOperationException("draw failed"))), Is.False);
            Assert.That(UPilotMainWindow.IsExitGUIException(
                new InvalidOperationException("not GUI control flow", exit)), Is.False);
            Assert.That(UPilotMainWindow.IsExitGUIException(null), Is.False);
        }

        public sealed class FallbackProbe : EditorWindow
        {
            public int Repaints;
            public Exception Failure;

            private void OnGUI()
            {
                // Layout intentionally has no fallback entries. The old GUILayout
                // HelpBox tried to consume nonexistent groups during Repaint.
                if (Event.current.type != EventType.Repaint)
                    return;
                try
                {
                    UPilotMainWindow.DrawExceptionFallback("draw failed",
                        new InvalidOperationException("fallback probe"), new Rect(Vector2.zero, position.size));
                    Repaints++;
                }
                catch (Exception ex)
                {
                    Failure = ex;
                    throw;
                }
            }
        }

        [UnityTest]
        public IEnumerator FallbackRepaintDoesNotRequireGUILayoutEntries()
        {
            var window = ScriptableObject.CreateInstance<FallbackProbe>();
            try
            {
                window.titleContent = new GUIContent("UPilot fallback probe");
                window.position = new Rect(150, 150, 320, 160);
                window.ShowUtility();
                for (var i = 0; i < 30 && window.Repaints < 2 && window.Failure == null; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                Assert.That(window.Failure, Is.Null);
                Assert.That(window.Repaints, Is.GreaterThanOrEqualTo(2), "The real IMGUI fallback must repaint.");
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                window.Close();
                if (window != null)
                    UnityEngine.Object.DestroyImmediate(window);
            }
        }
    }
}
