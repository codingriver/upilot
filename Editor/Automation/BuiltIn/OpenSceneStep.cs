namespace CodingRiver.UPilot.Automation
{
    [AutomationStep("upilot.open_scene", Description = "Open a saved scene in EditMode.", ArgumentsExample = "Assets/Scenes/Launch.unity")]
    public sealed class OpenSceneStep : AutomationStepBase
    {
        public override string Validate(string runId, string instanceId, string contextJson, string arguments) =>
            EditorStepOperations.ValidateScene(arguments, false);
        public override void Execute(string runId, string instanceId, string contextJson, string arguments)
        {
            EditorStepOperations.CheckOpen(arguments);
            SaveCheckpoint(runId, instanceId, "{\"requested\":true}");
            EditorStepOperations.Open(arguments);
        }
        public override string Poll(string runId, string instanceId, string contextJson, string arguments) => ResultJson(
            EditorStepOperations.Stable && EditorStepOperations.IsOpen(arguments) ? "Succeeded" : "Running");
        public override string Restore(string runId, string instanceId, string contextJson, string arguments) =>
            EditorStepOperations.Requested(contextJson) && EditorStepOperations.IsOpen(arguments)
                ? "{\"status\":\"Restored\"}" : "{\"status\":\"Unsupported\"}";
    }
}
