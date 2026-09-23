using System;
namespace CodingRiver.UPilot.Automation
{
    [AutomationStep("upilot.capture_snapshot", Description = "Capture and verify run-owned visual evidence.", TimeoutSeconds = 10)]
    public sealed class CaptureSnapshotStep : AutomationStepBase
    {
        public override string Validate(string runId, string instanceId, string contextJson, string arguments)
        {
            try { AutomationSnapshotEvidence.Parse(arguments); return ValidationOk(); }
            catch (Exception ex) { return ValidationError("STEP_SNAPSHOT_ARGUMENT_INVALID", ex.Message); }
        }
        public override void Execute(string runId, string instanceId, string contextJson, string arguments) =>
            BeginSnapshotJson(runId, instanceId, "snapshot", arguments);
        public override string Poll(string runId, string instanceId, string contextJson, string arguments) =>
            PollSnapshotJson(runId, instanceId, "snapshot");
        public override void Cancel(string runId, string instanceId, string contextJson, string arguments) =>
            CancelSnapshotJson(runId, instanceId, "snapshot");
        public override string GetError(string runId, string instanceId, string contextJson, string errorCode, string arguments) =>
            SnapshotErrorJson(runId, instanceId, "snapshot");
        public override string Restore(string runId, string instanceId, string contextJson, string arguments)
        {
            PollSnapshotJson(runId, instanceId, "snapshot");
            return "{\"status\":\"Restored\"}";
        }
    }
}
