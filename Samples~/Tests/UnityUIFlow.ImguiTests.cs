using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityUIFlow
{
    // Auto-compile trigger to recover from stuck test runner
    public sealed class UnityUIFlowImguiTests
    {
        // ── Selector Compiler Tests ──

        [Test]
        public void SelectorCompiler_ParseButtonType()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(button)");
            Assert.That(sel.Type, Is.EqualTo("button"));
            Assert.That(sel.Text, Is.Null);
            Assert.That(sel.Index, Is.EqualTo(-1));
        }

        [Test]
        public void SelectorCompiler_ParseButtonWithText()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(button, text=\"OK\")");
            Assert.That(sel.Type, Is.EqualTo("button"));
            Assert.That(sel.Text, Is.EqualTo("OK"));
        }

        [Test]
        public void SelectorCompiler_ParseTextFieldWithIndex()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(textfield, index=2)");
            Assert.That(sel.Type, Is.EqualTo("textfield"));
            Assert.That(sel.Index, Is.EqualTo(2));
        }

        [Test]
        public void SelectorCompiler_ParseGroupPath()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(group=\"Settings\" > button, text=\"Apply\")");
            Assert.That(sel.Group, Is.EqualTo("Settings"));
            Assert.That(sel.Type, Is.EqualTo("button"));
            Assert.That(sel.Text, Is.EqualTo("Apply"));
        }

        [Test]
        public void SelectorCompiler_ParseFocused()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(focused)");
            Assert.That(sel.Focused, Is.True);
        }

        [Test]
        public void SelectorCompiler_ParseControlName()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(textfield, control_name=\"username\")");
            Assert.That(sel.Type, Is.EqualTo("textfield"));
            Assert.That(sel.ControlName, Is.EqualTo("username"));
        }

        [Test]
        public void SelectorCompiler_ParseDropdown()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(dropdown, index=0)");
            Assert.That(sel.Type, Is.EqualTo("dropdown"));
            Assert.That(sel.Index, Is.EqualTo(0));
        }

        [Test]
        public void SelectorCompiler_ParseGroupOnly()
        {
            var sel = ImguiSelectorCompiler.Compile("gui(group=\"Main\")");
            Assert.That(sel.Group, Is.EqualTo("Main"));
            Assert.That(sel.Type, Is.EqualTo("group"));
        }

        [Test]
        public void SelectorCompiler_InvalidSelectorThrows()
        {
            Assert.Throws<UnityUIFlowException>(() => ImguiSelectorCompiler.Compile(""));
            Assert.Throws<UnityUIFlowException>(() => ImguiSelectorCompiler.Compile("not_a_selector"));
        }

        // ── Element Locator Tests ──

        [Test]
        public void Locator_FindFirst_ByType()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "button", Text = "Save", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "button", Text = "Cancel", GlobalIndex = 1 },
                new ImguiSnapshotEntry { InferredType = "textfield", GlobalIndex = 2 }
            );

            var sel = new ImguiSelector { Type = "button" };
            var result = ImguiElementLocator.Find(snapshot, sel);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Text, Is.EqualTo("Save"));
        }

        [Test]
        public void Locator_FindFirst_ByTypeAndText()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "button", Text = "Save", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "button", Text = "Cancel", GlobalIndex = 1 }
            );

            var sel = new ImguiSelector { Type = "button", Text = "Cancel" };
            var result = ImguiElementLocator.Find(snapshot, sel);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Text, Is.EqualTo("Cancel"));
        }

        [Test]
        public void Locator_FindFirst_ByIndex()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "textfield", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "textfield", GlobalIndex = 1 },
                new ImguiSnapshotEntry { InferredType = "textfield", GlobalIndex = 2 }
            );

            var sel = new ImguiSelector { Type = "textfield", Index = 1 };
            var result = ImguiElementLocator.Find(snapshot, sel);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.GlobalIndex, Is.EqualTo(1));
        }

        [Test]
        public void Locator_FindFirst_ByGroup()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "button", Text = "A", GroupName = "Header", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "button", Text = "B", GroupName = "Footer", GlobalIndex = 1 }
            );

            var sel = new ImguiSelector { Type = "button", Group = "Footer" };
            var result = ImguiElementLocator.Find(snapshot, sel);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Text, Is.EqualTo("B"));
        }

        [Test]
        public void Locator_NotFound_ReturnsNull()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "button", GlobalIndex = 0 }
            );

            var sel = new ImguiSelector { Type = "slider" };
            var result = ImguiElementLocator.Find(snapshot, sel);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Locator_FindAll_ByType()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "button", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "button", GlobalIndex = 1 },
                new ImguiSnapshotEntry { InferredType = "label", GlobalIndex = 2 }
            );

            var sel = new ImguiSelector { Type = "button" };
            var results = ImguiElementLocator.FindAll(snapshot, sel);

            Assert.That(results.Count, Is.EqualTo(2));
        }

        // ── Snapshot FindFirst/FindAll Tests ──

        [Test]
        public void Snapshot_FindFirst_ReturnsMatchingEntry()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "toggle", Text = "Enabled", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "toggle", Text = "Debug", GlobalIndex = 1 }
            );

            var sel = new ImguiSelector { Type = "toggle", Text = "Debug" };
            var result = snapshot.FindFirst(sel);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.GlobalIndex, Is.EqualTo(1));
        }

        [Test]
        public void Snapshot_FindAll_ReturnsOnlyMatches()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "slider", GlobalIndex = 0 },
                new ImguiSnapshotEntry { InferredType = "slider", GlobalIndex = 1 },
                new ImguiSnapshotEntry { InferredType = "button", GlobalIndex = 2 },
                new ImguiSnapshotEntry { InferredType = "slider", GlobalIndex = 3 }
            );

            var sel = new ImguiSelector { Type = "slider" };
            var results = snapshot.FindAll(sel);

            Assert.That(results.Count, Is.EqualTo(3));
        }

        [Test]
        public void Snapshot_FindFirst_NotFound_ReturnsNull()
        {
            var snapshot = CreateSnapshot(
                new ImguiSnapshotEntry { InferredType = "button", GlobalIndex = 0 }
            );

            var sel = new ImguiSelector { Type = "scroller" };
            var result = snapshot.FindFirst(sel);

            Assert.That(result, Is.Null);
        }

        // ── Integration Test: Snapshot Capture via MonoHook ──

        [UnityTest]
        public System.Collections.IEnumerator SnapshotCapture_RealWindow_HasEntries()
        {
            // Open the IMGUI example window
            var window = EditorWindow.GetWindow<UnityUIFlow.Examples.ImguiExampleWindow>();
            window.Show();
            window.Repaint();

            // Give Unity a few frames to repaint and for the hook to capture
            yield return null;
            yield return null;
            yield return null;

            // Get the bridge and check the snapshot
            var bridge = ImguiBridgeRegistry.GetOrCreateBridge(window);
            Assert.That(bridge, Is.Not.Null, "Bridge should be created");
            Assert.That(bridge.UsesMonoHookFallback, Is.True, "Should use MonoHook fallback in Unity 6000+");

            var snapshot = bridge.GetLastSnapshot();
            Assert.That(snapshot, Is.Not.Null, "Snapshot should be captured");
            Assert.That(snapshot.Entries, Is.Not.Null, "Snapshot entries should not be null");
            Assert.That(snapshot.Entries.Count, Is.GreaterThan(0), "Snapshot should have at least one entry");

            // Log entries for diagnostics
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[Test] Snapshot entries: {snapshot.Entries.Count}");
            foreach (var entry in snapshot.Entries)
            {
                sb.AppendLine($"  type={entry.InferredType}, text={entry.Text}, rect={entry.Rect}, group={entry.GroupName}");
            }
            Codingriver.Logger.Log(sb.ToString());

            window.Close();
        }

        // ── Helper ──

        private static ImguiSnapshot CreateSnapshot(params ImguiSnapshotEntry[] entries)
        {
            var snapshot = new ImguiSnapshot();
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].GlobalIndex = i;
                snapshot.Entries.Add(entries[i]);
            }
            return snapshot;
        }
    }
}
