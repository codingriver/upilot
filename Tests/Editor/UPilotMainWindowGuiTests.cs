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

        public sealed class AgentConfigurationProbe : EditorWindow
        {
            public Action Draw;
            public int Layouts;
            public double LastLayoutAt;
            public Exception Failure;

            private void OnGUI()
            {
                if (Draw == null || Failure != null)
                    return;
                try
                {
                    Draw();
                    if (Event.current.type == EventType.Layout)
                    {
                        Layouts++;
                        LastLayoutAt = EditorApplication.timeSinceStartup;
                    }
                }
                catch (Exception ex)
                {
                    Failure = ex;
                }
            }
        }

        [UnityTest]
        public IEnumerator DashboardLayoutsKeepAgentConfigurationUntilExplicitCheck()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            UPilotMainWindow main = null;
            AgentConfigurationProbe probe = null;
            try
            {
                main = ScriptableObject.CreateInstance<UPilotMainWindow>();
                probe = ScriptableObject.CreateInstance<AgentConfigurationProbe>();
                var mainView = typeof(UPilotMainWindow).GetField("_mainView", flags);
                mainView.SetValue(main, Enum.Parse(mainView.FieldType, "Dashboard"));
                Assert.That(mainView.GetValue(main).ToString(), Is.EqualTo("Dashboard"),
                    "Setup returns before the dashboard Layout path under test.");
                var fields = new[]
                {
                    typeof(UPilotMainWindow).GetField("_agentConfigs", flags),
                    typeof(UPilotMainWindow).GetField("_ruleConfigs", flags),
                    typeof(UPilotMainWindow).GetField("_skillConfigs", flags),
                };
                var snapshots = new object[fields.Length];
                for (var i = 0; i < fields.Length; i++)
                {
                    snapshots[i] = fields[i].GetValue(main);
                    Assert.That(((Array)snapshots[i]).Length, Is.GreaterThan(0),
                        "Opening the window must inspect each configuration category.");
                }
                var draw = typeof(UPilotMainWindow).GetMethod("DrawMainWindowGui", flags);
                probe.Draw = () => draw.Invoke(main, null);
                probe.titleContent = new GUIContent("UPilot configuration probe");
                probe.position = new Rect(150, 150, 520, 640);
                probe.ShowUtility();

                // Exercise real Layout events beyond the former polling interval.
                var afterPollingInterval = EditorApplication.timeSinceStartup + 2.1d;
                var deadline = afterPollingInterval + 8d;
                while (probe.Failure == null && EditorApplication.timeSinceStartup < deadline &&
                       (probe.Layouts < 2 || probe.LastLayoutAt < afterPollingInterval))
                {
                    probe.Repaint();
                    yield return null;
                }
                Assert.That(probe.Failure, Is.Null);
                Assert.That(probe.Layouts, Is.GreaterThanOrEqualTo(2));
                Assert.That(probe.LastLayoutAt, Is.GreaterThanOrEqualTo(afterPollingInterval));
                for (var i = 0; i < fields.Length; i++)
                    Assert.That(fields[i].GetValue(main), Is.SameAs(snapshots[i]),
                        "Dashboard drawing must retain the last explicit configuration check.");

                typeof(UPilotMainWindow).GetMethod("CheckAgentIntegrations", flags).Invoke(main, null);
                for (var i = 0; i < fields.Length; i++)
                {
                    Assert.That(fields[i].GetValue(main), Is.Not.SameAs(snapshots[i]),
                        "The existing Check Configuration action must inspect every category again.");
                    Assert.That(((Array)fields[i].GetValue(main)).Length, Is.GreaterThan(0));
                }
                Assert.That(typeof(UPilotMainWindow).GetField("_notice", flags).GetValue(main),
                    Does.StartWith("检查完成"));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                try
                {
                    if (probe != null)
                    {
                        probe.Draw = null;
                        probe.Close();
                        if (probe != null)
                            UnityEngine.Object.DestroyImmediate(probe);
                    }
                }
                finally
                {
                    // Health belongs to the verified service; closing a GUI fixture has no ownership.
                    if (main != null)
                        UnityEngine.Object.DestroyImmediate(main);
                }
            }
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
