using UnityEditor;
using UnityEditor.SceneManagement;

namespace CodingRiver.UPilot.Automation
{
    [AutomationStep("upilot.enter_play_mode", Description = "Enter PlayMode, optionally opening a saved scene first.", ArgumentsExample = "Assets/Scenes/Launch.unity")]
    public sealed class EnterPlayModeStep : AutomationStepBase
    {
        public override string Validate(string runId, string instanceId, string contextJson, string arguments) =>
            EditorStepOperations.ValidateScene(arguments, true);
        public override void Execute(string runId, string instanceId, string contextJson, string arguments)
        {
            if (arguments != "" && EditorApplication.isPlayingOrWillChangePlaymode)
            { Fail("STEP_EDIT_MODE_REQUIRED", "A specific launch scene requires EditMode."); return; }
            if (EditorApplication.isPlaying && EditorStepOperations.Stable)
            { SaveCheckpoint(runId, instanceId, "{\"requested\":true}"); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { Fail("STEP_MODE_TRANSITION_BUSY", "Another PlayMode transition is pending."); return; }
            if (arguments != "" && EditorSceneManager.playModeStartScene != null
                && AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene) != arguments)
            { Fail("STEP_START_SCENE_CONFLICT", "The configured PlayMode startup scene conflicts with the requested scene."); return; }
            EditorStepOperations.Prepare();
            if (arguments != "") EditorStepOperations.Open(arguments);
            SaveCheckpoint(runId, instanceId, "{\"requested\":true}");
            EditorStepOperations.Mode(true);
        }
        public override string Poll(string runId, string instanceId, string contextJson, string arguments)
        {
            if (!IsRunning) return base.Poll(runId, instanceId, contextJson, arguments);
            return ResultJson(EditorApplication.isPlaying && EditorStepOperations.Stable ? "Succeeded" : "Running");
        }
        public override void Cancel(string runId, string instanceId, string contextJson, string arguments)
        {
            if (!EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode)
                EditorStepOperations.Mode(false);
        }
        public override string Cleanup(string runId, string instanceId, string contextJson, string arguments) =>
            ResultJson(EditorStepOperations.Stable ? "Succeeded" : "Running");
        public override string Restore(string runId, string instanceId, string contextJson, string arguments) =>
            EditorStepOperations.Requested(contextJson) ? "{\"status\":\"Restored\"}" : "{\"status\":\"Unsupported\"}";
    }
}
