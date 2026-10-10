// -----------------------------------------------------------------------
// UPilot Editor — https://github.com/codingriver/upilot
// SPDX-License-Identifier: MIT
// -----------------------------------------------------------------------

using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace CodingRiver.UPilot.Tests
{
    /// <summary>
    /// Deliberately mixed leaves for P2-WP-08's real Runner observation
    /// matrix.  All leaves are Explicit, so they never enter a normal package
    /// run; callers must select these exact fully-qualified test names.
    /// </summary>
    public sealed class UPilotRunnerLeafAcceptanceFixtures
    {
        [Test, Explicit("P2-WP-08 targeted real-Runner pass leaf."), Order(1)]
        public void PassLeaf()
        {
            Assert.That(2 + 2, Is.EqualTo(4));
        }

        [Test, Explicit("P2-WP-08 targeted real-Runner expected failure leaf."), Order(2)]
        public void FailLeaf()
        {
            Assert.Fail("P2_WP08_EXPECTED_LEAF_FAILURE");
        }

        [UnityTest, Explicit("P2-WP-08 targeted real-Runner delayed leaf."), Order(3), Timeout(45000)]
        public IEnumerator DelayedLeaf()
        {
            // Leave enough time for a fresh Streamable HTTP client to attach
            // and observe the earlier failed leaf while this leaf is pending.
            double deadline = EditorApplication.timeSinceStartup + 30.0;
            while (EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(EditorApplication.timeSinceStartup, Is.GreaterThanOrEqualTo(deadline));
        }
    }

    // Uses UTF's public post-build hook, not framework task interception. Explicit-only:
    // temporarily replaces clean, saved scenes and restores them after RunFinished.
    [PostBuildCleanup(typeof(UPilotConsoleSceneRestoreAcceptance))]
    public sealed class UPilotConsoleSceneRestoreAcceptance : IPostBuildCleanup
    {
        private static UnityEditor.SceneManagement.SceneSetup[] _originalScenes;

        // Called once before starting the exact explicit test: UTF switches scenes before
        // test methods execute, so the real user layout must be observed beforehand.
        public static void Arm()
        {
            string project = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, ".."))
                .Replace('\\', '/');
            Assert.That(project, Is.EqualTo("D:/upilot/Tests~/UPilotTest").IgnoreCase);
            Assert.That(_originalScenes, Is.Null);
            var setup = UnityEditor.SceneManagement.EditorSceneManager.GetSceneManagerSetup();
            Assert.That(setup.Length, Is.EqualTo(1), "This probe only restores a single clean saved scene.");
            Assert.That(setup[0].isLoaded && setup[0].isActive, Is.True);
            foreach (var item in setup)
            {
                Assert.That(item.path, Is.Not.Empty, "Never discard an unsaved scene.");
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(item.path);
                if (scene.IsValid()) Assert.That(scene.isDirty, Is.False, "Never discard scene edits.");
            }
            _originalScenes = setup;
        }

        [Test, Explicit("Stage4 real cleanup/sceneRestore Console boundary acceptance; call Arm before the exact run.")]
        public void SceneRestorationIsObservedAfterRunFinished()
        {
            Assert.That(_originalScenes, Is.Not.Null, "Arm the probe before starting UTF.");
            Assert.That(_originalScenes.Length, Is.GreaterThan(0));
            // Force a real open during cleanup; restoring an already-loaded scene is a no-op.
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Single);
            UnityEngine.Debug.Log("STAGE4_PHASE_EXECUTION");
        }

        public void Cleanup()
        {
            var setup = _originalScenes;
            if (setup == null) return; // Preflight failure made no scene change.
            bool cleanupPending = UPilotTestService.Instance.GetStatusSnapshot().cleanupPending;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpening += LogSceneOpening;
            try
            {
                UnityEngine.Debug.Log("STAGE4_PHASE_CLEANUP_BEFORE_RESTORE");
                // Restore through the public open path: native RestoreSceneManagerSetup can
                // bypass sceneOpening/sceneOpened on this Unity version (reported as a gap).
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    setup[0].path, UnityEditor.SceneManagement.OpenSceneMode.Single);
                _originalScenes = null;
                UnityEngine.Debug.Log("STAGE4_PHASE_CLEANUP_AFTER_RESTORE");
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.sceneOpening -= LogSceneOpening;
            }
            Assert.That(cleanupPending, Is.True, "The probe must restore scenes after the public RunFinished callback.");
        }

        private static void LogSceneOpening(string path, UnityEditor.SceneManagement.OpenSceneMode mode)
        {
            UnityEngine.Debug.Log("STAGE4_PHASE_SCENE_RESTORE:" + path);
        }
    }

}
