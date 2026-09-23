namespace CodingRiver.UPilot.Automation
{
    [AutomationStep("upilot.console_capture_start", Description = "Start run-owned Console Capture; executor stops it after Finally.", TimeoutSeconds = 10)]
    public sealed class ConsoleCaptureStartStep : AutomationStepBase
    {
        public override string Validate(string runId, string instanceId, string contextJson, string arguments) =>
            arguments == "" ? ValidationOk() : ValidationError("STEP_ARGUMENTS_INVALID", "Capture start takes an empty string.");
        public override void Execute(string runId, string instanceId, string contextJson, string arguments)
        { UPilotAutomationStepService.StartRunCapture(runId, instanceId); Succeed(); }
        public override string Restore(string runId, string instanceId, string contextJson, string arguments)
        { UPilotAutomationStepService.ObserveRunCapture(runId, instanceId); Succeed(); return "{\"status\":\"Restored\"}"; }
    }
}
