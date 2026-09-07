using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CodingRiver.UPilot.Tests
{
    public class UPilotSceneSummaryTests
    {
        [Test]
        public void SummaryCountsKnownSceneAndCapsExamples()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Root");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.AddComponent<Camera>();
                root.AddComponent<Light>();
                var child = new GameObject("Inactive");
                child.transform.SetParent(root.transform);
                child.SetActive(false);
                var result = UPilotSceneSummaryService.Summarize(new[] { scene }, new SceneSummaryRequest { maxMilliseconds = 1000, maxExamples = 1 });
                Assert.That(result.coverageComplete, Is.True);
                Assert.That(result.nodesVisited, Is.EqualTo(2));
                Assert.That(result.activeNodesVisited, Is.EqualTo(1));
                Assert.That(result.componentsVisited, Is.EqualTo(4));
                Assert.That(result.cameras, Is.EqualTo(1));
                Assert.That(result.lights, Is.EqualTo(1));
                Assert.That(result.examples, Has.Count.EqualTo(1));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void WideTreeStopsAtNodeBudgetWithoutClaimingCompleteCoverage()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Root");
                SceneManager.MoveGameObjectToScene(root, scene);
                for (int index = 0; index < 500; index++) new GameObject("Child").transform.SetParent(root.transform);
                var result = UPilotSceneSummaryService.Summarize(new[] { scene }, new SceneSummaryRequest { maxNodes = 3, maxMilliseconds = 1000 });
                Assert.That(result.nodesVisited, Is.EqualTo(3));
                Assert.That(result.truncated, Is.True);
                Assert.That(result.coverageComplete, Is.False);
                Assert.That(result.truncationReason, Is.EqualTo("node_budget"));
                Assert.That(result.countScope, Is.EqualTo("visited_nodes_only"));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
