using System;
using System.Collections;
using System.IO;
using System.Linq;
using CodingRiver.UPilot.Automation;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
using static CodingRiver.UPilot.Tests.Automation.AutomationStepRegistryTests;

namespace CodingRiver.UPilot.Tests.Automation
{
    public class AutomationBuiltInStepTests
    {
        [TestCase("-1")] [TestCase("NaN")] [TestCase("Infinity")] [TestCase("text")]
        public void WaitRejectsInvalidSeconds(string seconds)
        { Assert.That(AutomationStepJsonCodec.Validation(new WaitSecondsStep().Validate("", "wait", "{}", seconds)).ok, Is.False); }
        [Test]
        public void SceneValidationIsReadOnlyAndIndependentOfCurrentMode()
        {
            var before = EditorSceneManager.GetSceneManagerSetup();
            Assert.That(AutomationStepJsonCodec.Validation(new OpenSceneStep().Validate("", "scene", "{}", "../invalid.unity")).ok, Is.False);
            Assert.That(AutomationStepJsonCodec.Validation(new EnterPlayModeStep().Validate("", "play", "{}", "")).ok, Is.True);
            Assert.That(AutomationStepJsonCodec.Validation(new EnterEditModeStep().Validate("", "edit", "{}", "")).ok, Is.True);
            Assert.That(EditorSceneManager.GetSceneManagerSetup().Length, Is.EqualTo(before.Length));
        }
        [Test]
        public void SceneStepsBlockDirtyScenesAndConflictingStartupWithoutSwitchingMode()
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            bool restoreDefaultScene = false;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                Assert.That(loaded.isDirty, Is.False, "The fixture must not replace unsaved user changes.");
                if (loaded.path == "" && loaded.rootCount != 0)
                {
                    var roots = loaded.GetRootGameObjects();
                    Assert.That(roots.Length == 2 && roots.Any(o => o.name == "Main Camera" && o.GetComponent<Camera>() != null)
                        && roots.Any(o => o.name == "Directional Light" && o.GetComponent<Light>() != null),
                        Is.True, "Only the clean default Test Runner scene may be replaced.");
                    restoreDefaultScene = true;
                }
            }
            var startup = EditorSceneManager.playModeStartScene;
            string path = "Assets/UPilotStepTest_" + Guid.NewGuid().ToString("N") + ".unity";
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
                EditorSceneManager.MarkSceneDirty(scene);
                var exception = Assert.Throws<AutomationStepException>(() => new OpenSceneStep().Execute("", "scene", "{}", path));
                Assert.That(exception.Code, Is.EqualTo("STEP_SCENE_UNSAVED"));
                Assert.That(scene.isDirty, Is.True);
                Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
                EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                var play = new EnterPlayModeStep();
                play.Execute("", "play", "{}", "Assets/ConflictingScene.unity");
                Assert.That(AutomationStepJsonCodec.Result(play.Poll("", "play", "{}", "Assets/ConflictingScene.unity")).errorCode, Is.EqualTo("STEP_START_SCENE_CONFLICT"));
                Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
                Assert.That(AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene), Is.EqualTo(path));
            }
            finally
            {
                EditorSceneManager.playModeStartScene = startup;
                try
                {
                    if (setup.Length == 0 || setup.All(s => string.IsNullOrEmpty(s.path)))
                        EditorSceneManager.NewScene(restoreDefaultScene ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    else EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
                finally { AssetDatabase.DeleteAsset(path); }
            }
        }
        [UnityTest]
        public IEnumerator WaitCompletesWithoutBlockingEditor()
        {
            var path = Path.Combine(Application.dataPath, "../Library/UPilot/wait-test.json");
            using var executor = new AutomationStepExecutor(new AutomationStepRegistry(), path);
            executor.Start(Plan(Item("upilot.wait_seconds", "0.05")), "wait-test");
            int frames = 0;
            double deadline = EditorApplication.timeSinceStartup + 5;
            while (!executor.State.terminal && EditorApplication.timeSinceStartup < deadline)
            { executor.Tick(); frames++; yield return null; }
            Assert.That(executor.State.status, Is.EqualTo("Succeeded"));
            Assert.That(frames, Is.GreaterThan(1));
            File.Delete(path);
        }
    }
}
