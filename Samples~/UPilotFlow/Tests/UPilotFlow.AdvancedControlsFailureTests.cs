using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using UnityEditor.UIElements;

namespace CodingRiver.UPilot.Flow
{
    public sealed class UPilotFlowAdvancedControlsFailureTests : UPilotFlowFixture<AdvancedControlsWindow>
    {
        [UnityTest]
        public IEnumerator SetBoundValueAction_ThrowsOnInvalidElement()
        {
            yield return null;
            Task task = ExecuteActionAsync(new SetBoundValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#enabled-toggle",
                ["value"] = "true",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator AssertBoundValueAction_ThrowsOnMismatch()
        {
            yield return null;
            Task task = ExecuteActionAsync(new AssertBoundValueAction(), new Dictionary<string, string>
            {
                ["selector"] = "#property-title-field",
                ["expected"] = "DefinitelyNotThisValue",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator SelectTreeItemAction_ThrowsOnInvalidId()
        {
            yield return null;
            Task task = ExecuteActionAsync(new SelectTreeItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#navigation-tree",
                ["id"] = "99999",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator SelectTabAction_ThrowsOnInvalidLabel()
        {
            yield return null;
            Task task = ExecuteActionAsync(new SelectTabAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-tabs",
                ["label"] = "NonExistentTab",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator CloseTabAction_ThrowsOnInvalidLabel()
        {
            yield return null;
            Task task = ExecuteActionAsync(new CloseTabAction(), new Dictionary<string, string>
            {
                ["selector"] = "#settings-tabs",
                ["label"] = "NonExistentTab",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator SortColumnAction_ThrowsOnInvalidColumn()
        {
            yield return null;
            VisualElement multiColumn = Root.Q<VisualElement>("multi-column-list");
            if (multiColumn == null)
            {
                Assert.Ignore("MultiColumnListView not available in this Unity version.");
            }

            Task task = ExecuteActionAsync(new SortColumnAction(), new Dictionary<string, string>
            {
                ["selector"] = "#multi-column-list",
                ["column"] = "NonExistentColumn",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator ResizeColumnAction_ThrowsOnInvalidIndex()
        {
            yield return null;
            VisualElement multiColumn = Root.Q<VisualElement>("multi-column-list");
            if (multiColumn == null)
            {
                Assert.Ignore("MultiColumnListView not available in this Unity version.");
            }

            Task task = ExecuteActionAsync(new ResizeColumnAction(), new Dictionary<string, string>
            {
                ["selector"] = "#multi-column-list",
                ["index"] = "999",
                ["width"] = "200",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator NavigateBreadcrumbAction_ThrowsOnInvalidLabel()
        {
            yield return null;
            VisualElement breadcrumbs = Root.Q<VisualElement>("toolbar-breadcrumbs");
            if (breadcrumbs == null)
            {
                Assert.Ignore("ToolbarBreadcrumbs not available in this Unity version.");
            }

            Task task = ExecuteActionAsync(new NavigateBreadcrumbAction(), new Dictionary<string, string>
            {
                ["selector"] = "#toolbar-breadcrumbs",
                ["label"] = "NonExistent",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator ReadBreadcrumbsAction_ThrowsWhenMissing()
        {
            yield return null;
            Task task = ExecuteActionAsync(new ReadBreadcrumbsAction(), new Dictionary<string, string>
            {
                ["selector"] = "#nonexistent-breadcrumbs",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator SetSplitViewSizeAction_ThrowsOnInvalidPane()
        {
            yield return null;
            Task task = ExecuteActionAsync(new SetSplitViewSizeAction(), new Dictionary<string, string>
            {
                ["selector"] = "#workspace-split",
                ["pane"] = "5",
                ["size"] = "100",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator PageScrollerAction_ThrowsOnInvalidDirection()
        {
            yield return null;
            Task task = ExecuteActionAsync(new PageScrollerAction(), new Dictionary<string, string>
            {
                ["selector"] = "#standalone-scroller",
                ["direction"] = "sideways",
                ["pages"] = "1",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator DragScrollerAction_ThrowsOnInvalidRatio()
        {
            yield return null;
            Task task = ExecuteActionAsync(new DragScrollerAction(), new Dictionary<string, string>
            {
                ["selector"] = "#standalone-scroller",
                ["ratio"] = "abc",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator DragReorderAction_ThrowsOnInvalidIndex()
        {
            yield return null;
            Task task = ExecuteActionAsync(new DragReorderAction(), new Dictionary<string, string>
            {
                ["selector"] = "#item-list",
                ["from_index"] = "999",
                ["to_index"] = "0",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }

        [UnityTest]
        public IEnumerator ClickPopupItemAction_ThrowsOnInvalidValue()
        {
            yield return null;
            Task task = ExecuteActionAsync(new ClickPopupItemAction(), new Dictionary<string, string>
            {
                ["selector"] = "#quick-popup",
                ["value"] = "NonExistentValue",
            });
            yield return UPilotFlowTestTaskUtility.AwaitFailure(task, ex =>
            {
                Assert.That(ex, Is.TypeOf<UPilotFlowException>());
            });
        }
    }
}
