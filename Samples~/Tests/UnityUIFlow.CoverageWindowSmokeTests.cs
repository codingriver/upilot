using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using UnityUIFlow.Examples;

namespace UnityUIFlow
{
    public sealed class UnityUIFlowCoverageWindowSmokeTests
    {
        [UnityTest]
        public IEnumerator ExampleCoverageFieldsWindow_OpensAndContainsKeyControls()
        {
            var window = EditorWindow.GetWindow<ExampleCoverageFieldsWindow>();
            window.Show();
            yield return null;

            VisualElement root = window.rootVisualElement;
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Q<VisualElement>("advanced-controls-host"), Is.Not.Null);
            Assert.That(root.Q<DropdownField>("choice-dropdown"), Is.Not.Null);
            Assert.That(root.Q<Toggle>("enabled-toggle"), Is.Not.Null);
            Assert.That(root.Q<MaskField>("feature-mask"), Is.Not.Null);
            Assert.That(root.Q<EnumFlagsField>("permissions-flags"), Is.Not.Null);
            Assert.That(root.Q<Slider>("volume-slider"), Is.Not.Null);
            Assert.That(root.Q<ColorField>("accent-color"), Is.Not.Null);
            Assert.That(root.Q<Vector3Field>("offset-vector3"), Is.Not.Null);

            window.Close();
        }

        [UnityTest]
        public IEnumerator ExampleCoverageCollectionsWindow_OpensAndContainsKeyControls()
        {
            var window = EditorWindow.GetWindow<ExampleCoverageCollectionsWindow>();
            window.Show();
            yield return null;

            VisualElement root = window.rootVisualElement;
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Q<VisualElement>("advanced-controls-host"), Is.Not.Null);
            Assert.That(root.Q<ListView>("item-list"), Is.Not.Null);
            Assert.That(root.Q<TreeView>("navigation-tree"), Is.Not.Null);
            Assert.That(root.Q<VisualElement>("planet-list"), Is.Not.Null);
            Assert.That(root.Q<VisualElement>("scene-tree"), Is.Not.Null);
            Assert.That(root.Q<VisualElement>("workspace-split"), Is.Not.Null);
            Assert.That(root.Q<Scroller>("standalone-scroller"), Is.Not.Null);
            Assert.That(root.Q<TabView>("settings-tabs"), Is.Not.Null);

            window.Close();
        }

        [UnityTest]
        public IEnumerator ExampleCoverageInputWindow_OpensAndContainsKeyControls()
        {
            var window = EditorWindow.GetWindow<ExampleCoverageInputWindow>();
            window.Show();
            yield return null;

            VisualElement root = window.rootVisualElement;
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Q<VisualElement>("advanced-controls-host"), Is.Not.Null);
            Assert.That(root.Q<Toolbar>("main-toolbar"), Is.Not.Null);
            Assert.That(root.Q<ToolbarMenu>("toolbar-menu"), Is.Not.Null);
            Assert.That(root.Q<ToolbarSearchField>("toolbar-search"), Is.Not.Null);
            Assert.That(root.Q<VisualElement>("toolbar-breadcrumbs"), Is.Not.Null);
            Assert.That(root.Q<VisualElement>("gesture-drag-target"), Is.Not.Null);
            Assert.That(root.Q<TextField>("keyboard-input"), Is.Not.Null);
            Assert.That(root.Q<VisualElement>("context-target"), Is.Not.Null);

            window.Close();
        }
    }
}
