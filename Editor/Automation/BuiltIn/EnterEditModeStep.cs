using UnityEditor;

namespace CodingRiver.UPilot.Automation
{
    [AutomationStep("upilot.enter_edit_mode", Description = "Exit PlayMode, optionally opening a return scene.", ArgumentsExample = "")]
    public sealed class EnterEditModeStep : AutomationStepBase
    {
        public override string Validate(string runId, string instanceId, string contextJson, string arguments) =>
            EditorStepOperations.ValidateScene(arguments, true);
        public override void Execute(string runId, string instanceId, string contextJson, string arguments)
        {
            SaveCheckpoint(runId, instanceId, "{\"requested\":true}");
            if (EditorApplication.isPlayingOrWillChangePlaymode) EditorStepOperations.Mode(false);
        }
        public override string Poll(string runId, string instanceId, string contextJson, string arguments)
        {
            if (EditorApplication.isPlaying || !EditorStepOperations.Stable)
                return ResultJson("Running");
            if (arguments != "" && !EditorStepOperations.IsOpen(arguments))
                EditorStepOperations.Open(arguments);
            return ResultJson("Succeeded");
        }
        public override string Cleanup(string runId, string instanceId, string contextJson, string arguments) =>
            ResultJson(EditorStepOperations.Stable ? "Succeeded" : "Running");
        public override string Restore(string runId, string instanceId, string contextJson, string arguments) =>
            EditorStepOperations.Requested(contextJson) ? "{\"status\":\"Restored\"}" : "{\"status\":\"Unsupported\"}";
    }
}
